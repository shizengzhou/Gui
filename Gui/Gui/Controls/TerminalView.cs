using System;
using System.Collections.Generic;
using System.Threading;
using Gui.Terminal;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Gui.Controls;

/// <summary>
/// Draws a libghostty-vt viewport with Win2D and forwards keyboard input to the PTY.
/// </summary>
public sealed class TerminalView : UserControl
{
    private const string FontFamilyName = "Cascadia Mono";
    private const float FontSizeDip = 14f;

    private readonly TerminalSession _session = new();
    private readonly CanvasControl _canvas;
    private readonly Dictionary<CellAttrs, CanvasTextFormat> _formats = new();
    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _resizeTimer;

    private CanvasTextFormat? _measureFormat;
    private float _cellWidth = 8f;
    private float _cellHeight = 18f;
    private float _textOffsetY;
    private bool _started;
    private bool _cursorOn = true;
    private int _pendingFrame;
    private string? _pendingHighSurrogate;

    public TerminalSession Session => _session;

    public event Action<string>? TitleChanged;

    public TerminalView()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;

        _canvas = new CanvasControl
        {
            ClearColor = Color.FromArgb(255, 27, 27, 27),
            UseSharedDevice = true,
        };
        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += OnCanvasSizeChanged;
        Content = _canvas;

        KeyDown += OnKeyDown;
        CharacterReceived += OnCharacterReceived;
        PointerPressed += OnPointerPressed;
        PointerWheelChanged += OnPointerWheelChanged;
        Unloaded += OnUnloaded;

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blinkTimer.Tick += OnBlinkTick;
        _blinkTimer.Start();

        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _resizeTimer.Tick += OnResizeTick;

        _session.OutputReady += OnOutputReady;
        _session.TitleChanged += OnSessionTitleChanged;
    }

    private void OnSessionTitleChanged()
    {
        // The callback fires inside ghostty_terminal_vt_write, so read the title on the UI thread.
        DispatcherQueue.TryEnqueue(() =>
        {
            string title = _session.ReadTitle();
            if (title.Length > 0)
                TitleChanged?.Invoke(title);
        });
    }

    private void OnOutputReady()
    {
        if (Interlocked.Exchange(ref _pendingFrame, 1) != 0)
            return;
        DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _pendingFrame, 0);
            _canvas.Invalidate();
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _blinkTimer.Stop();
        _resizeTimer.Stop();
        _canvas.RemoveFromVisualTree();
    }

    // ---------------- rendering ----------------

    private void EnsureMetrics(CanvasControl sender)
    {
        if (_measureFormat is not null)
            return;

        _measureFormat = new CanvasTextFormat
        {
            FontFamily = FontFamilyName,
            FontSize = FontSizeDip,
            WordWrapping = CanvasWordWrapping.NoWrap,
            Options = CanvasDrawTextOptions.EnableColorFont,
        };

        using var layout = new CanvasTextLayout(sender, "MMMMMMMMMM", _measureFormat, 0f, 0f);
        float advance;
        try
        {
            var caret = layout.GetCaretPosition(1, false);
            advance = caret.X;
        }
        catch
        {
            advance = (float)layout.LayoutBounds.Width / 10f;
        }

        _cellWidth = MathF.Max(1f, MathF.Round(advance));
        _cellHeight = MathF.Round(FontSizeDip * 1.32f);
        _textOffsetY = MathF.Round((_cellHeight - (float)layout.LayoutBounds.Height) / 2f);
    }

    private CanvasTextFormat GetFormat(CellAttrs attrs)
    {
        if (_formats.TryGetValue(attrs, out var cached))
            return cached;

        var format = new CanvasTextFormat
        {
            FontFamily = FontFamilyName,
            FontSize = FontSizeDip,
            WordWrapping = CanvasWordWrapping.NoWrap,
            FontWeight = (attrs & CellAttrs.Bold) != 0
                ? Microsoft.UI.Text.FontWeights.Bold
                : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = (attrs & CellAttrs.Italic) != 0
                ? Windows.UI.Text.FontStyle.Italic
                : Windows.UI.Text.FontStyle.Normal,
        };
        _formats[attrs] = format;
        return format;
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        EnsureMetrics(sender);

        var ds = args.DrawingSession;
        var snapshot = _session.Capture();

        int cols = Math.Max(1, (int)(sender.ActualWidth / _cellWidth));
        int rows = Math.Max(1, (int)(sender.ActualHeight / _cellHeight));

        if (!_started)
        {
            float scale = (float)(XamlRoot?.RasterizationScale ?? 1.0);
            _session.Start(cols, rows, (int)(_cellWidth * scale), (int)(_cellHeight * scale));
            _started = true;
            snapshot = _session.Capture();
        }

        ds.FillRectangle(new Rect(0, 0, sender.ActualWidth, sender.ActualHeight), ToColor(snapshot.DefaultBg));

        for (int y = 0; y < snapshot.RowData.Length; y++)
        {
            var row = snapshot.RowData[y];
            float rowTop = y * _cellHeight;
            foreach (var run in row.Runs)
            {
                float left = run.Col * _cellWidth;
                float width = run.Width * _cellWidth;

                if (run.Bg != snapshot.DefaultBg)
                    ds.FillRectangle(new Rect(left, rowTop, width, _cellHeight), ToColor(run.Bg));

                var color = ToColor(run.Fg);
                ds.DrawText(run.Text, left, rowTop + _textOffsetY, color, GetFormat(run.Attrs));

                if ((run.Attrs & CellAttrs.Underline) != 0)
                {
                    float uy = rowTop + _cellHeight - 2f;
                    ds.DrawLine(left, uy, left + width, uy, color, 1f);
                }

                if ((run.Attrs & CellAttrs.Strikethrough) != 0)
                {
                    float sy = rowTop + _cellHeight * 0.55f;
                    ds.DrawLine(left, sy, left + width, sy, color, 1f);
                }
            }
        }

        DrawCursor(ds, snapshot);
    }

    private void DrawCursor(CanvasDrawingSession ds, TerminalSnapshot snapshot)
    {
        if (!snapshot.CursorVisible || !snapshot.CursorInViewport)
            return;
        if (snapshot.CursorBlinking && !_cursorOn)
            return;

        float left = snapshot.CursorX * _cellWidth;
        float top = snapshot.CursorY * _cellHeight;
        var color = ToColor(snapshot.CursorColor);

        switch (snapshot.CursorStyle)
        {
            case 0: // bar
                ds.FillRectangle(new Rect(left, top, 2f, _cellHeight), color);
                break;
            case 2: // underline
                ds.FillRectangle(new Rect(left, top + _cellHeight - 2f, _cellWidth, 2f), color);
                break;
            case 3: // hollow block
                ds.DrawRectangle(new Rect(left + 0.5f, top + 0.5f, _cellWidth - 1f, _cellHeight - 1f), color, 1f);
                break;
            default: // block
                ds.FillRectangle(new Rect(left, top, _cellWidth, _cellHeight), Color.FromArgb(110, color.R, color.G, color.B));
                break;
        }
    }

    private static Color ToColor(TermColor c) => Color.FromArgb(255, c.R, c.G, c.B);

    private void OnBlinkTick(object? sender, object e)
    {
        _cursorOn = !_cursorOn;
        _canvas.Invalidate();
    }

    // ---------------- sizing ----------------

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _resizeTimer.Stop();
        _resizeTimer.Start();
        _canvas.Invalidate();
    }

    private void OnResizeTick(object? sender, object e)
    {
        _resizeTimer.Stop();
        if (!_started)
            return;

        int cols = Math.Max(1, (int)(_canvas.ActualWidth / _cellWidth));
        int rows = Math.Max(1, (int)(_canvas.ActualHeight / _cellHeight));
        float scale = (float)(XamlRoot?.RasterizationScale ?? 1.0);
        _session.Resize(cols, rows, (int)(_cellWidth * scale), (int)(_cellHeight * scale));
        _canvas.Invalidate();
    }

    // ---------------- input ----------------

    private static bool IsDown(VirtualKey key)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(_canvas).Properties.MouseWheelDelta;
        _session.Scroll(delta > 0 ? -3 : 3);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsDown(VirtualKey.Control);
        bool alt = IsDown(VirtualKey.Menu);
        bool shift = IsDown(VirtualKey.Shift);

        if (ctrl && shift && e.Key == VirtualKey.V)
        {
            _ = PasteClipboardAsync();
            e.Handled = true;
            return;
        }

        byte[]? bytes = TerminalInput.Encode(e.Key, ctrl, alt, shift);
        if (bytes is not null)
        {
            _session.Write(bytes);
            e.Handled = true;
        }
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (IsDown(VirtualKey.Control) && !IsDown(VirtualKey.Menu))
            return;

        char ch = args.Character;
        if (ch < 0x20 || ch == 0x7F)
            return;

        if (char.IsHighSurrogate(ch))
        {
            _pendingHighSurrogate = ch.ToString();
            args.Handled = true;
            return;
        }

        string text;
        if (char.IsLowSurrogate(ch) && _pendingHighSurrogate is not null)
        {
            text = _pendingHighSurrogate + ch;
            _pendingHighSurrogate = null;
        }
        else
        {
            _pendingHighSurrogate = null;
            text = ch.ToString();
        }

        _session.Write(TerminalInput.EncodeText(text));
        args.Handled = true;
    }

    private async System.Threading.Tasks.Task PasteClipboardAsync()
    {
        try
        {
            var view = Clipboard.GetContent();
            if (view.Contains(StandardDataFormats.Text))
                _session.Write(await view.GetTextAsync());
        }
        catch
        {
            // Clipboard unavailable.
        }
    }
}
