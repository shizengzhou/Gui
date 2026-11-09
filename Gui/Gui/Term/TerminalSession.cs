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
/// One horizontal search highlight in viewport coordinates: zero-based row, inclusive
/// start/end columns, and whether it is the currently selected match.
/// </summary>
public readonly record struct SearchSpan(int Row, int StartX, int EndX, bool Selected);

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
    private IntPtr _mouseEncoder;
    private IntPtr _mouseEvent;
    private IntPtr _search;
    private IntPtr _rowIterator;
    private IntPtr _cells;
    private ConPty? _pty;
    private bool _disposed;

    private readonly List<byte> _pending = new(64 * 1024);

    // Delegates must stay alive for as long as the native terminal holds them.
    private readonly GhosttyNative.WritePtyFn _writePty;
    private readonly GhosttyNative.NotifyFn _titleChanged;
    private readonly GhosttyNative.NotifyFn _pwdChanged;
    private readonly GhosttyNative.NotifyFn _bell;
    private readonly GhosttyNative.ClipboardFn _clipboardWrite;
    private readonly GhosttyNative.ClipboardFn _clipboardRead;

    // "clipboard-write" / "clipboard-read": allow, deny, or ask. "ask" has no prompt in this
    // build (the reply must be synchronous), so it behaves like deny.
    private string _clipboardWritePolicy = "allow";
    private string _clipboardReadPolicy = "ask";

    // Last pwd reported by the shell over OSC 7 / OSC 9;9. UI-thread only.
    private string _oscPwd = string.Empty;
    private string _shellExecutable = string.Empty;

    // OSC 133 marks emitted by the injected shell integration. UI-thread only.
    private readonly ShellMarkParser _marks = new();

    /// <summary>Marshals work onto the UI thread. Must not block; called from the reader thread.</summary>
    public Action<Action>? Post;

    public Action? OnUpdate;
    public Action? OnOutput;
    public Action<string>? OnTitle;
    public Action? OnBell;
    public Action? OnExit;

    /// <summary>OSC 133 D: a command finished, with its exit code and runtime.</summary>
    public Action<int, TimeSpan>? OnCommandFinished;

    /// <summary>True between the command's output start (C) and its finish (D).</summary>
    public bool CommandRunning => _marks.CommandRunning;

    public IntPtr Terminal => _terminal;
    public IntPtr RenderState => _renderState;

    public bool Running => _pty is not null;

    public TerminalSession()
    {
        _writePty = OnWritePty;
        _titleChanged = OnTitleChanged;
        _pwdChanged = OnPwdChanged;
        _bell = OnBellChanged;
        _clipboardWrite = OnClipboardWrite;
        _clipboardRead = OnClipboardRead;
        _marks.CommandFinished = (exit, duration) => OnCommandFinished?.Invoke(exit, duration);

        if (GhosttyNative.ghostty_terminal_new(IntPtr.Zero, out _terminal, 80, 24) != GhosttyNative.SUCCESS)
            throw new InvalidOperationException("ghostty_terminal_new failed");
        if (GhosttyNative.ghostty_search_new(IntPtr.Zero, out _search, _terminal) != GhosttyNative.SUCCESS)
            _search = IntPtr.Zero;
        if (GhosttyNative.ghostty_render_state_new(IntPtr.Zero, out _renderState) != GhosttyNative.SUCCESS)
            throw new InvalidOperationException("ghostty_render_state_new failed");
        if (GhosttyNative.ghostty_key_encoder_new(IntPtr.Zero, out _keyEncoder) != GhosttyNative.SUCCESS)
            throw new InvalidOperationException("ghostty_key_encoder_new failed");
        if (GhosttyNative.ghostty_mouse_encoder_new(IntPtr.Zero, out _mouseEncoder) != GhosttyNative.SUCCESS)
            _mouseEncoder = IntPtr.Zero;
        if (GhosttyNative.ghostty_mouse_event_new(IntPtr.Zero, out _mouseEvent) != GhosttyNative.SUCCESS)
            _mouseEvent = IntPtr.Zero;
        if (GhosttyNative.ghostty_render_state_row_iterator_new(IntPtr.Zero, out _rowIterator) != GhosttyNative.SUCCESS)
            throw new InvalidOperationException("ghostty_render_state_row_iterator_new failed");
        if (GhosttyNative.ghostty_render_state_row_cells_new(IntPtr.Zero, out _cells) != GhosttyNative.SUCCESS)
            throw new InvalidOperationException("ghostty_render_state_row_cells_new failed");

        SetFunctionPointer(GhosttyNative.OPT_WRITE_PTY, _writePty);
        SetFunctionPointer(GhosttyNative.OPT_TITLE_CHANGED, _titleChanged);
        SetFunctionPointer(GhosttyNative.OPT_PWD_CHANGED, _pwdChanged);
        SetFunctionPointer(GhosttyNative.OPT_BELL, _bell);
        SetFunctionPointer(GhosttyNative.OPT_CLIPBOARD_WRITE, _clipboardWrite);
        SetFunctionPointer(GhosttyNative.OPT_CLIPBOARD_READ, _clipboardRead);

        ApplyTheme(Theme.Terminal);
    }

    /// <summary>
    /// "clipboard-write" / "clipboard-read" policies and the per-write byte limit
    /// ("clipboard-write-limit-bytes"). The limit is enforced natively before the callback
    /// runs, so a program cannot make the app buffer more than it allows.
    /// </summary>
    public unsafe void ApplyClipboardSettings(string read, string write, ulong writeLimitBytes)
    {
        _clipboardReadPolicy = read;
        _clipboardWritePolicy = write;
        if (_terminal == IntPtr.Zero) return;

        nuint limit = writeLimitBytes == ulong.MaxValue ? nuint.MaxValue : (nuint)writeLimitBytes;
        GhosttyNative.ghostty_terminal_set(
            _terminal, GhosttyNative.OPT_CLIPBOARD_WRITE_MAX_BYTES, &limit);
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

        // A color-only change does not dirty the existing rows, so the old render state keeps
        // their resolved colors and background tabs would paint the previous theme. Rebuild
        // the state so the next frame resolves every cell against the new palette.
        ResetRenderState();
    }

    /// <summary>Drops and recreates the render state so the next frame is fully re-resolved.</summary>
    private void ResetRenderState()
    {
        if (_renderState != IntPtr.Zero)
        {
            GhosttyNative.ghostty_render_state_free(_renderState);
            _renderState = IntPtr.Zero;
        }
        if (GhosttyNative.ghostty_render_state_new(IntPtr.Zero, out _renderState) != GhosttyNative.SUCCESS)
            _renderState = IntPtr.Zero;
    }

    /// <summary>Spawns the shell inside a ConPTY of the given character grid size.</summary>
    public bool Start(string command, string workingDirectory, ushort cols, ushort rows, CellMetrics metrics,
        IReadOnlyList<(string Name, string Value)>? environment, string term, string input,
        string shellIntegration, ShellIntegrationFeatures shellFeatures)
    {
        if (_terminal == IntPtr.Zero || _pty is not null) return false;

        if (string.IsNullOrEmpty(command)) command = DefaultShellPath();
        string executable = ExecutableToken(command);
        _shellExecutable = executable;
        // Only augment a bare shell command: any configured arguments are left exactly
        // as the user wrote them. An auto-detected shell is an unquoted full path that may
        // contain spaces, so "bare" compares against the executable, not the first token.
        if (IsPowerShell(command) && command.Trim().Length == executable.Length)
            command = ShellIntegration.Inject(command, shellIntegration, shellFeatures);

        try
        {
            Diag.Debug("pty.start", $"cols={cols} rows={rows} exec={executable}");
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

    /// <summary>
    /// Applies output buffered while no pane was attached. A tab that moves to another
    /// window has no pane for a moment; the new one flushes that backlog on attach.
    /// </summary>
    internal void FlushPending() => Post?.Invoke(Drain);

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
        // The terminal consumes the OSC sequences; read them from the same bytes first.
        _marks.Feed(bytes);
        OnOutput?.Invoke();
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
        _marks.Reset();
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

    private void OnBellChanged(IntPtr terminal, IntPtr userdata) => OnBell?.Invoke();

    // ---------------- clipboard (OSC 52 / kitty) ----------------

    private unsafe void OnClipboardWrite(IntPtr terminal, IntPtr userdata, IntPtr request)
    {
        var write = *(GhosttyNative.GhosttyClipboardWrite*)request;
        int result = ClipboardAllowed(write.Granted != 0, _clipboardWritePolicy)
            ? WriteClipboard(in write)
            : GhosttyNative.CLIPBOARD_WRITE_DENIED;

        var reply = new GhosttyNative.GhosttyClipboardWriteReply
        {
            Size = (nuint)sizeof(GhosttyNative.GhosttyClipboardWriteReply),
            Result = result,
        };
        Reply(request, write.Reply, &reply);
    }

    private static unsafe int WriteClipboard(in GhosttyNative.GhosttyClipboardWrite request)
    {
        // An empty write clears the destination.
        if (request.ContentsLen == 0)
        {
            return ClipboardAccess.TryClear()
                ? GhosttyNative.CLIPBOARD_WRITE_SUCCESS
                : GhosttyNative.CLIPBOARD_WRITE_IO_ERROR;
        }
        if (request.Contents == IntPtr.Zero) return GhosttyNative.CLIPBOARD_WRITE_UNSUPPORTED;

        var contents = (GhosttyNative.GhosttyClipboardContent*)request.Contents;
        for (nuint i = 0; i < request.ContentsLen; i++)
        {
            GhosttyNative.GhosttyClipboardContent item = contents[i];
            if (!IsTextMime(ReadString(item.Mime))) continue;
            return ClipboardAccess.TryWriteText(ReadString(item.Data))
                ? GhosttyNative.CLIPBOARD_WRITE_SUCCESS
                : GhosttyNative.CLIPBOARD_WRITE_IO_ERROR;
        }

        // Only text is served; kitty protocol images and other MIME types are not.
        return GhosttyNative.CLIPBOARD_WRITE_UNSUPPORTED;
    }

    private unsafe void OnClipboardRead(IntPtr terminal, IntPtr userdata, IntPtr request)
    {
        var read = *(GhosttyNative.GhosttyClipboardRead*)request;

        if (!ClipboardAllowed(read.Granted != 0, _clipboardReadPolicy))
        {
            var denied = NewReadReply(GhosttyNative.CLIPBOARD_READ_DENIED);
            Reply(request, read.Reply, &denied);
            return;
        }

        // Serve the first requested MIME this build can answer; it only has text.
        string? mime = null;
        var mimes = (GhosttyNative.GhosttyString*)read.Mimes;
        for (nuint i = 0; i < read.MimesLen; i++)
        {
            string candidate = ReadString(mimes[i]);
            if (!IsTextMime(candidate)) continue;
            mime = candidate;
            break;
        }

        if (mime is null)
        {
            var unsupported = NewReadReply(read.MimesLen == 0
                ? GhosttyNative.CLIPBOARD_READ_SUCCESS
                : GhosttyNative.CLIPBOARD_READ_UNSUPPORTED);
            Reply(request, read.Reply, &unsupported);
            return;
        }

        if (!ClipboardAccess.TryReadText(out string text))
        {
            var ioError = NewReadReply(GhosttyNative.CLIPBOARD_READ_IO_ERROR);
            Reply(request, read.Reply, &ioError);
            return;
        }

        byte[] mimeBytes = Encoding.UTF8.GetBytes(mime);
        byte[] dataBytes = Encoding.UTF8.GetBytes(text);
        fixed (byte* mimePointer = mimeBytes)
        fixed (byte* dataPointer = dataBytes)
        {
            var content = new GhosttyNative.GhosttyClipboardContent
            {
                Mime = new GhosttyNative.GhosttyString
                {
                    Ptr = (IntPtr)mimePointer,
                    Len = (nuint)mimeBytes.Length,
                },
                Data = new GhosttyNative.GhosttyString
                {
                    Ptr = (IntPtr)dataPointer,
                    Len = (nuint)dataBytes.Length,
                },
            };
            var reply = new GhosttyNative.GhosttyClipboardReadReply
            {
                Size = (nuint)sizeof(GhosttyNative.GhosttyClipboardReadReply),
                Result = GhosttyNative.CLIPBOARD_READ_SUCCESS,
                Contents = (IntPtr)(&content),
                ContentsLen = 1,
            };
            Reply(request, read.Reply, &reply);
        }
    }

    private static GhosttyNative.GhosttyClipboardReadReply NewReadReply(int result) =>
        new()
        {
            Size = (nuint)Marshal.SizeOf<GhosttyNative.GhosttyClipboardReadReply>(),
            Result = result,
        };

    /// <summary>
    /// "ask" needs a synchronous prompt, which this build cannot show without re-entering the
    /// VT stream; it denies instead. A session grant (kitty password) always allows.
    /// </summary>
    private static bool ClipboardAllowed(bool granted, string policy) =>
        granted || policy == "allow";

    private static bool IsTextMime(string mime) =>
        mime is "text/plain" or "text/plain;charset=utf-8" or "UTF8_STRING" or "TEXT" or "STRING";

    private static unsafe string ReadString(GhosttyNative.GhosttyString value) =>
        value.Ptr == IntPtr.Zero || value.Len == 0
            ? string.Empty
            : Encoding.UTF8.GetString((byte*)value.Ptr, checked((int)value.Len));

    private static unsafe void Reply(IntPtr request, IntPtr replyPointer, void* reply)
    {
        if (replyPointer == IntPtr.Zero) return;
        var callback = Marshal.GetDelegateForFunctionPointer<GhosttyNative.ClipboardReplyFn>(replyPointer);
        callback(request, (IntPtr)reply);
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
        Diag.Debug("pwd", _oscPwd);
    }

    // ---------------- search ----------------

    /// <summary>
    /// Starts (or clears) a text search. Empty or null returns the search to idle; changing
    /// the needle restarts the scan from scratch. All calls run on the UI thread, which also
    /// mutates the terminal, so no extra locking is needed.
    /// </summary>
    public bool SearchSetNeedle(string? needle)
    {
        if (_search == IntPtr.Zero) return false;
        if (string.IsNullOrEmpty(needle))
            return GhosttyNative.ghostty_search_set(_search, GhosttyNative.SEARCH_OPT_NEEDLE, null)
                   == GhosttyNative.SUCCESS;

        byte[] bytes = Encoding.UTF8.GetBytes(needle);
        fixed (byte* pointer = bytes)
        {
            var value = new GhosttyNative.GhosttyString
            {
                Ptr = (IntPtr)pointer,
                Len = (nuint)bytes.Length,
            };
            return GhosttyNative.ghostty_search_set(_search, GhosttyNative.SEARCH_OPT_NEEDLE, &value)
                   == GhosttyNative.SUCCESS;
        }
    }

    /// <summary>
    /// Advances the scan with a small time budget so typing never blocks on a large
    /// scrollback. Returns true once the search is caught up with the terminal.
    /// </summary>
    public bool SearchStep()
    {
        if (_search == IntPtr.Zero) return true;

        long deadline = Environment.TickCount64 + 8;
        for (int guard = 0; guard < 10000; guard++)
        {
            int status = -1;
            if (GhosttyNative.ghostty_search_tick(_search, &status) != GhosttyNative.SUCCESS)
                return true;

            if (status == GhosttyNative.SEARCH_STATUS_COMPLETE) return true;
            if (status == GhosttyNative.SEARCH_STATUS_RUNNING)
            {
                if (Environment.TickCount64 >= deadline) return false;
                continue;
            }

            // FEED_REQUIRED: copy the next chunk of terminal data, then keep ticking.
            if (GhosttyNative.ghostty_search_feed(_search) != GhosttyNative.SUCCESS) return true;
            if (Environment.TickCount64 >= deadline) return false;
        }
        return false;
    }

    /// <summary>Selects the next (older) or previous (newer) match, scrolling it into view.</summary>
    public bool SearchSelect(bool next)
    {
        if (_search == IntPtr.Zero) return false;
        int option = next ? GhosttyNative.SEARCH_OPT_SELECT_NEXT : GhosttyNative.SEARCH_OPT_SELECT_PREV;
        return GhosttyNative.ghostty_search_set(_search, option, null) == GhosttyNative.SUCCESS;
    }

    /// <summary>Total matches and the selected match index (0 = newest); null when none is selected.</summary>
    public void SearchStats(out ulong total, out ulong? selected)
    {
        total = 0;
        selected = null;
        if (_search == IntPtr.Zero) return;

        ulong count = 0;
        if (GhosttyNative.ghostty_search_get(_search, GhosttyNative.SEARCH_DATA_TOTAL_MATCHES, &count)
            == GhosttyNative.SUCCESS)
        {
            total = count;
        }

        ulong index = 0;
        if (GhosttyNative.ghostty_search_get(_search, GhosttyNative.SEARCH_DATA_SELECTED_INDEX, &index)
            == GhosttyNative.SUCCESS)
        {
            selected = index;
        }
    }

    /// <summary>
    /// The highlight rectangles for the current viewport. The cached viewport list is
    /// refreshed with a feed so manual scrolling does not draw yesterday's matches, and
    /// each match is converted from screen coordinates so partially visible matches clip
    /// to the visible rows.
    /// </summary>
    public List<SearchSpan> SearchSpans()
    {
        var spans = new List<SearchSpan>();
        if (_search == IntPtr.Zero || _terminal == IntPtr.Zero) return spans;

        GhosttyNative.ghostty_search_feed(_search);

        ushort cols = 80;
        GhosttyNative.ghostty_terminal_get(_terminal, GhosttyNative.TDATA_COLS, &cols);
        if (!Scrollbar(out _, out ulong offset, out ulong len) || len == 0) return spans;

        if (TryGetSelections(GhosttyNative.SEARCH_DATA_VIEWPORT_MATCHES, out var matches))
        {
            foreach (GhosttyNative.GhosttySelection match in matches)
                AddSelectionSpans(spans, match, cols, offset, len, false);
        }

        var selected = new GhosttyNative.GhosttySelection
        {
            Size = (nuint)sizeof(GhosttyNative.GhosttySelection),
        };
        if (GhosttyNative.ghostty_search_get(_search, GhosttyNative.SEARCH_DATA_SELECTED_MATCH, &selected)
            == GhosttyNative.SUCCESS)
        {
            AddSelectionSpans(spans, selected, cols, offset, len, true);
        }

        return spans;
    }

    /// <summary>Reads a GhosttySelectionBuffer field, growing the array to the reported size.</summary>
    private bool TryGetSelections(int data, out GhosttyNative.GhosttySelection[] selections)
    {
        selections = Array.Empty<GhosttyNative.GhosttySelection>();

        var query = new GhosttyNative.GhosttySelectionBuffer();
        int result = GhosttyNative.ghostty_search_get(_search, data, &query);
        if (query.Len == 0)
            return result == GhosttyNative.SUCCESS || result == GhosttyNative.NO_VALUE;
        if (result != GhosttyNative.OUT_OF_SPACE) return false;

        var items = new GhosttyNative.GhosttySelection[(int)query.Len];
        fixed (GhosttyNative.GhosttySelection* pointer = items)
        {
            var buffer = new GhosttyNative.GhosttySelectionBuffer
            {
                Ptr = (IntPtr)pointer,
                Cap = query.Len,
                Len = 0,
            };
            if (GhosttyNative.ghostty_search_get(_search, data, &buffer) != GhosttyNative.SUCCESS)
                return false;

            int count = (int)buffer.Len;
            if (count < items.Length) Array.Resize(ref items, count);
            selections = items;
            return true;
        }
    }

    /// <summary>
    /// Converts a match to viewport-relative rows. Search selections are linear ranges;
    /// a range can span soft-wrapped rows, so each covered row contributes its own span.
    /// </summary>
    private void AddSelectionSpans(List<SearchSpan> spans, GhosttyNative.GhosttySelection selection,
        ushort cols, ulong offset, ulong viewportRows, bool selected)
    {
        GhosttyNative.GhosttyPointCoordinate start;
        GhosttyNative.GhosttyPointCoordinate end;
        if (GhosttyNative.ghostty_terminal_point_from_grid_ref(
                _terminal, in selection.Start, GhosttyNative.POINT_SCREEN, &start) != GhosttyNative.SUCCESS)
        {
            return;
        }
        if (GhosttyNative.ghostty_terminal_point_from_grid_ref(
                _terminal, in selection.End, GhosttyNative.POINT_SCREEN, &end) != GhosttyNative.SUCCESS)
        {
            return;
        }

        uint firstRow = start.Y;
        uint lastRow = end.Y;
        ushort firstX = start.X;
        ushort lastX = end.X;
        if (firstRow > lastRow || (firstRow == lastRow && firstX > lastX))
        {
            (firstRow, lastRow) = (lastRow, firstRow);
            (firstX, lastX) = (lastX, firstX);
        }

        for (uint row = firstRow; row <= lastRow; row++)
        {
            long viewportRow = (long)row - (long)offset;
            if (viewportRow < 0 || viewportRow >= (long)viewportRows) continue;

            int startX = row == firstRow ? firstX : 0;
            int endX = row == lastRow ? lastX : Math.Max(0, cols - 1);
            if (endX < startX) continue;

            spans.Add(new SearchSpan((int)viewportRow, startX, endX, selected));
            if (row == uint.MaxValue) break;
        }
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
        return ApplySelection(anchor, cursor);
    }

    /// <summary>
    /// Selection endpoints in screen (scrollback-absolute) rows. A drag that auto-scrolls
    /// the viewport keeps the anchor on the same text, unlike viewport-relative rows.
    /// </summary>
    public bool SetSelectionScreen(ushort anchorX, uint anchorY, ushort cursorX, uint cursorY)
    {
        if (_terminal == IntPtr.Zero) return false;

        var anchor = new GhosttyNative.GhosttyPoint { Tag = GhosttyNative.POINT_SCREEN, X = anchorX, Y = anchorY };
        var cursor = new GhosttyNative.GhosttyPoint { Tag = GhosttyNative.POINT_SCREEN, X = cursorX, Y = cursorY };
        return ApplySelection(anchor, cursor);
    }

    private bool ApplySelection(GhosttyNative.GhosttyPoint anchor, GhosttyNative.GhosttyPoint cursor)
    {
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

        Diag.Debug("session.resize", $"cols={cols} rows={rows} pty={_pty is not null}");
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

    /// <summary>Scrolls the viewport to the newest row ("scroll-to-bottom").</summary>
    public void ScrollToBottom()
    {
        if (_terminal == IntPtr.Zero) return;
        var behavior = new GhosttyNative.GhosttyTerminalScrollViewport
        {
            Tag = GhosttyNative.SCROLL_BOTTOM,
        };
        GhosttyNative.ghostty_terminal_scroll_viewport(_terminal, behavior);
        OnUpdate?.Invoke();
    }

    /// <summary>
    /// Encodes one mouse event into the escape sequence the running program asked for.
    /// The position is in cell coordinates: the encoder's default 1x1 cell geometry then
    /// maps it straight to a terminal cell. False when nothing was reported (the terminal
    /// is not in a mouse tracking mode).
    /// </summary>
    public bool EncodeMouse(int action, int button, int mods, double cellX, double cellY,
        out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (_mouseEncoder == IntPtr.Zero || _mouseEvent == IntPtr.Zero || _terminal == IntPtr.Zero)
            return false;

        // Tracking mode and wire format come from the terminal's current modes.
        if (GhosttyNative.ghostty_mouse_encoder_setopt_from_terminal(_mouseEncoder, _terminal)
            != GhosttyNative.SUCCESS)
        {
            return false;
        }

        GhosttyNative.ghostty_mouse_event_set_action(_mouseEvent, action);
        if (button > 0) GhosttyNative.ghostty_mouse_event_set_button(_mouseEvent, button);
        else GhosttyNative.ghostty_mouse_event_clear_button(_mouseEvent);
        GhosttyNative.ghostty_mouse_event_set_mods(_mouseEvent, (ushort)mods);
        GhosttyNative.ghostty_mouse_event_set_position(_mouseEvent,
            new GhosttyNative.GhosttyMousePosition { X = (float)cellX, Y = (float)cellY });

        var buffer = new byte[256];
        fixed (byte* output = buffer)
        {
            int result = GhosttyNative.ghostty_mouse_encoder_encode(
                _mouseEncoder, _mouseEvent, output, (nuint)buffer.Length, out nuint written);
            if (result != GhosttyNative.SUCCESS || written == 0) return false;

            bytes = buffer.AsSpan(0, (int)written).ToArray();
            return true;
        }
    }

    /// <summary>The OSC 8 hyperlink URI at a viewport cell, or null when there is none.</summary>
    public string? HyperlinkAt(ushort x, ushort y)
    {
        if (_terminal == IntPtr.Zero) return null;

        var point = new GhosttyNative.GhosttyPoint
        {
            Tag = GhosttyNative.POINT_VIEWPORT,
            X = x,
            Y = y,
        };
        var gridRef = default(GhosttyNative.GhosttyGridRef);
        gridRef.Size = (nuint)sizeof(GhosttyNative.GhosttyGridRef);
        if (GhosttyNative.ghostty_terminal_grid_ref(_terminal, point, &gridRef)
            != GhosttyNative.SUCCESS)
        {
            return null;
        }

        if (GhosttyNative.ghostty_grid_ref_hyperlink_uri(in gridRef, null, 0, out nuint needed)
            != GhosttyNative.OUT_OF_SPACE || needed == 0)
        {
            return null;
        }

        var buffer = new byte[(int)needed];
        fixed (byte* data = buffer)
        {
            if (GhosttyNative.ghostty_grid_ref_hyperlink_uri(
                    in gridRef, data, (nuint)buffer.Length, out nuint written) != GhosttyNative.SUCCESS ||
                written == 0)
            {
                return null;
            }
            return Encoding.UTF8.GetString(buffer, 0, (int)written);
        }
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
        string token = ExecutableToken(command);
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
    /// The executable part of a shell command. An auto-detected shell is an unquoted full
    /// path that can contain spaces, so the whole string wins when it names a real file;
    /// otherwise the command is "exe args" and the first token is the executable.
    /// </summary>
    public static string ExecutableToken(string command)
    {
        string trimmed = command.Trim();
        return File.Exists(trimmed) ? trimmed : FirstToken(command);
    }

    // OSC 7 sends a file:// URI, OSC 9;9 (ConEmu) sends a quoted Windows path. Only OSC 7
    // reaches this app: libghostty-vt maps it to the terminal's pwd, and treats OSC 9 as a
    // notification.
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
        if (_mouseEvent != IntPtr.Zero) { GhosttyNative.ghostty_mouse_event_free(_mouseEvent); _mouseEvent = IntPtr.Zero; }
        if (_mouseEncoder != IntPtr.Zero) { GhosttyNative.ghostty_mouse_encoder_free(_mouseEncoder); _mouseEncoder = IntPtr.Zero; }
        if (_keyEncoder != IntPtr.Zero) { GhosttyNative.ghostty_key_encoder_free(_keyEncoder); _keyEncoder = IntPtr.Zero; }
        if (_search != IntPtr.Zero) { GhosttyNative.ghostty_search_free(_search); _search = IntPtr.Zero; }
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
